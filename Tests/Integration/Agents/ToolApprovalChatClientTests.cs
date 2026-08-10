using Domain.Contracts;
using Domain.DTOs;
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
public class ToolApprovalChatClientTests(McpVaultServerFixture mcpFixture, RedisFixture redisFixture)
    : IClassFixture<McpVaultServerFixture>, IClassFixture<RedisFixture>
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

    private McpAgent CreateAgent(ToolApprovalChatClient approvalClient)
    {
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, TimeSpan.FromMinutes(10));
        return new McpAgent(
            TestAgentSpec.Default with { McpServerEndpoints = [mcpFixture.McpEndpoint] },
            approvalClient,
            stateStore,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            [],
            []);
    }

    // Only the stall retry here, not the bad-answer one: what these tests assert on is what the
    // approval handler recorded, so a second turn would append to a list the assertions read.
    private Task<List<AiResponse>> RunAsync(
        ToolApprovalChatClient approvalClient, string prompt, TimeSpan? budget = null) =>
        LlmAttempt.WithinAsync(budget ?? LlmAttempt.Budget, async ct =>
        {
            await using var agent = CreateAgent(approvalClient);
            return await agent.RunStreamingAsync(prompt, cancellationToken: ct)
                .ToUpdateAiResponsePairs()
                .Where(x => x.Item2 is not null)
                .Select(x => x.Item2!)
                .ToListAsync(ct);
        });

    [Fact]
    public async Task Agent_WithApprovalRequired_TerminatesWhenRejected()
    {
        // Arrange
        var innerClient = CreateLlmClient();
        var rejectingHandler = new TestApprovalHandler(result: ToolApprovalResult.Rejected);
        var approvalClient = new ToolApprovalChatClient(innerClient, rejectingHandler, "conv-test");

        mcpFixture.CreateFile("ApprovalTestMovies/placeholder.txt");

        // Act
        var responses = await RunAsync(approvalClient,
            "IMPORTANT: You MUST call a tool right now. Use your file search/glob tool to find all files with pattern **/*. Do NOT respond with text, just call the tool immediately.");

        // Assert - should terminate with rejection message
        responses.ShouldNotBeEmpty();
        rejectingHandler.RequestedApprovals.ShouldNotBeEmpty();
        rejectingHandler.RequestedApprovals[0][0].ToolName.ShouldContain("glob");
    }

    [Fact]
    public async Task Agent_WithWhitelistedTool_SkipsApprovalForWhitelistedTools()
    {
        // Arrange
        var innerClient = CreateLlmClient();
        var rejectingHandler = new TestApprovalHandler(result: ToolApprovalResult.Rejected);
        var approvalClient = new ToolApprovalChatClient(
            innerClient,
            rejectingHandler, "conv-test",
            whitelistPatterns: ["*__fs_*"]);

        mcpFixture.CreateFile("WhitelistTestMovies/placeholder.txt");

        // Act
        var responses = await RunAsync(approvalClient,
            "IMPORTANT: You MUST call a tool right now. Use your file search/glob tool to find all files with pattern **/*. Do NOT respond with text, just call the tool immediately.");

        // Assert
        responses.ShouldNotBeEmpty();
        rejectingHandler.RequestedApprovals.ShouldBeEmpty("Whitelisted tool should not require approval");
        var hasContent = responses.Any(r => !string.IsNullOrEmpty(r.Content) || !string.IsNullOrEmpty(r.ToolCalls));
        hasContent.ShouldBeTrue();
    }

    [Fact]
    public async Task Agent_WithMixedTools_OnlyRequestsApprovalForNonWhitelisted()
    {
        // Arrange
        var innerClient = CreateLlmClient();
        var approvingHandler = new TestApprovalHandler(result: ToolApprovalResult.Approved);
        var approvalClient = new ToolApprovalChatClient(
            innerClient,
            approvingHandler, "conv-test",
            whitelistPatterns: ["*__fs_glob"]);

        mcpFixture.CreateFile(Path.Combine("MixedTestSource", "test-file.mkv"), "content");
        mcpFixture.CreateFile("MixedTestDest/placeholder.txt");

        var sourcePath = Path.Combine(mcpFixture.VaultPath, "MixedTestSource", "test-file.mkv");
        var destPath = Path.Combine(mcpFixture.VaultPath, "MixedTestDest", "test-file.mkv");

        // Act
        var responses = await RunAsync(approvalClient,
            $"First find all .mkv files using your glob tool with pattern **/*.mkv, then move '{sourcePath}' to '{destPath}'.",
            budget: TimeSpan.FromSeconds(180));

        // Assert
        responses.ShouldNotBeEmpty();
        var approvedToolNames = approvingHandler.RequestedApprovals
            .SelectMany(r => r.Select(t => t.ToolName))
            .ToList();
        approvedToolNames.ShouldNotContain(n => n.Contains("fs_glob"), "Whitelisted tool should not be in approval requests");
    }

    private sealed class TestApprovalHandler(ToolApprovalResult result) : IToolApprovalHandler
    {
        public List<IReadOnlyList<ToolApprovalRequest>> RequestedApprovals { get; } = [];

        public Task<ToolApprovalResult> RequestApprovalAsync(
            string conversationId,
            IReadOnlyList<ToolApprovalRequest> requests,
            CancellationToken cancellationToken)
        {
            RequestedApprovals.Add(requests);
            return Task.FromResult(result);
        }

        public Task NotifyAutoApprovedAsync(
            string conversationId,
            IReadOnlyList<ToolApprovalRequest> requests,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}