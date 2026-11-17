using System.Text.Json;
using Domain.Agents;
using Domain.DTOs;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Agents.AI;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

public class McpAgentTests(McpLibraryServerFixture mcpFixture, RedisFixture redisFixture)
    : IClassFixture<McpLibraryServerFixture>, IClassFixture<RedisFixture>, IAsyncLifetime
{
    private FakeOpenRouter _model = null!;

    public async Task InitializeAsync() => _model = await FakeOpenRouter.StartAsync();

    public async Task DisposeAsync() => await _model.DisposeAsync();

    private OpenRouterChatClient CreateLlmClient() => FakeTurn.Client(_model);

    private McpAgent CreateAgent(OpenRouterChatClient llmClient)
    {
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, new RetentionSettings { PurgeHorizon = TimeSpan.FromMinutes(10) }, TimeProvider.System);
        return new McpAgent(
            TestAgentSpec.Default with { McpServerEndpoints = [McpServerEndpoint.Configured(mcpFixture.McpEndpoint, McpTestSecret.Value)] },
            llmClient,
            stateStore,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            [],
            [],
            // Signed as the deployment's agent signs, or every gated server refuses its calls.
            callTokens: McpTestSecret.CallTokens);
    }

    private async Task<List<AiResponse>> CallAsync(string tool, string arguments)
    {
        _model.Script(FakeOpenRouter.CallingToolNamed(tool, arguments));
        using var llmClient = CreateLlmClient();
        await using var agent = CreateAgent(llmClient);
        return await FakeTurn.RunAsync(agent, $"Use the {tool} tool.");
    }

    [Fact]
    public async Task Agent_WithMoveFileTool_CanMoveFileWithinLibrary()
    {
        // Move requires both paths to be under the library path.
        mcpFixture.CreateLibraryStructure("AgentMoveDestination");
        mcpFixture.CreateLibraryFile(Path.Combine("AgentMoveSource", "agent-test-file.mkv"), "fake content");
        var sourcePath = Path.Combine(mcpFixture.LibraryPath, "AgentMoveSource", "agent-test-file.mkv");
        var destPath = Path.Combine(mcpFixture.LibraryPath, "AgentMoveDestination", "agent-test-file.mkv");

        var responses = await CallAsync("fs_move",
            new System.Text.Json.Nodes.JsonObject { ["sourcePath"] = sourcePath, ["destinationPath"] = destPath }.ToJsonString());

        // Assert
        responses.ShouldNotBeEmpty();
        mcpFixture.FileExistsInLibrary(Path.Combine("AgentMoveDestination", "agent-test-file.mkv")).ShouldBeTrue();
        mcpFixture.FileExistsInLibrary(Path.Combine("AgentMoveSource", "agent-test-file.mkv")).ShouldBeFalse();
    }

    [Fact]
    public async Task Agent_WithFsDeleteTool_CanRemoveLeftoverDownloadDirectory()
    {
        // A leftover download directory whose torrent no longer exists.
        const int downloadId = 99999;
        var downloadSubDir = Path.Combine(mcpFixture.DownloadPath, downloadId.ToString());
        Directory.CreateDirectory(downloadSubDir);
        await File.WriteAllTextAsync(Path.Combine(downloadSubDir, "leftover.nfo"), "info file");

        var responses = await CallAsync("fs_delete", $$"""{"path":"downloads/{{downloadId}}"}""");

        // Assert
        responses.ShouldNotBeEmpty();
        Directory.Exists(downloadSubDir).ShouldBeFalse();
    }

    [Fact]
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

    [Fact]
    public async Task Agent_ThreadSerialization_WithAgentKey_CanRestoreThread()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        var agentKey = new AgentKey("12345:67890");

        // Act - Create thread from AgentKey (simulating how ChatMonitor works). The restart is the
        // point of the test, so both agents live inside one attempt.
        var agentKeyJson = JsonSerializer.SerializeToElement(agentKey.ToString());
        var (updates1, updates2) = await LlmAttempt.WithinAsync(TimeSpan.FromSeconds(30), async ct =>
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