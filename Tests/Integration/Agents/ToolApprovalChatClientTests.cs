using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

// The fake model calls the glob tool, as it answers the demand below, and the approval client decides.
public class ToolApprovalChatClientTests(McpVaultServerFixture mcpFixture, RedisFixture redisFixture)
    : IClassFixture<McpVaultServerFixture>, IClassFixture<RedisFixture>, IAsyncLifetime
{
    private FakeOpenRouter _model = null!;

    public async Task InitializeAsync() => _model = await FakeOpenRouter.StartAsync();

    public async Task DisposeAsync() => await _model.DisposeAsync();

    private OpenRouterChatClient CreateLlmClient() => FakeTurn.Client(_model);

    private McpAgent CreateAgent(ToolApprovalChatClient approvalClient)
    {
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, new RetentionSettings { PurgeHorizon = TimeSpan.FromMinutes(10) }, TimeProvider.System);
        return new McpAgent(
            TestAgentSpec.Default with { McpServerEndpoints = [McpServerEndpoint.Configured(mcpFixture.McpEndpoint, McpTestSecret.Value)] },
            approvalClient,
            stateStore,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            [],
            [],
            // Signed as the deployment's agent signs, or every gated server refuses its calls.
            callTokens: McpTestSecret.CallTokens);
    }

    private async Task<List<AiResponse>> RunAsync(ToolApprovalChatClient approvalClient, string prompt)
    {
        await using var agent = CreateAgent(approvalClient);
        return await FakeTurn.RunAsync(agent, prompt);
    }

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
        _model.Requests[^1].ToolOutputs.ShouldHaveSingleItem().ShouldContain("placeholder.txt");
    }

    private sealed class TestApprovalHandler(ToolApprovalResult result) : IToolApprovalHandler
    {
        public List<IReadOnlyList<ToolApprovalRequest>> RequestedApprovals { get; } = [];

        public Task<ToolApprovalResult> RequestApprovalAsync(ApprovalAsk ask, CancellationToken cancellationToken)
        {
            RequestedApprovals.Add(ask.Requests);
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