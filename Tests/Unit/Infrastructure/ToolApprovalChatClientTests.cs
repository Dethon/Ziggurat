using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Shouldly;
using Tests.Unit.Infrastructure.Helpers;
using static Tests.Unit.Infrastructure.Helpers.ToolApprovalResponseFactory;

namespace Tests.Unit.Infrastructure;

public class ToolApprovalChatClientTests
{
    [Fact]
    public async Task InvokeFunctionAsync_WhenNotWhitelisted_RequestsApproval()
    {
        // Arrange
        var handler = new TestApprovalHandler(result: ToolApprovalResult.Approved);
        var invoked = false;
        var function = AIFunctionFactory.Create(() =>
        {
            invoked = true;
            return "result";
        }, "mcp__server__TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp__server__TestTool", "call1"));

        var client = new ToolApprovalChatClient(fakeClient, handler, "conv-test");
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        handler.RequestedApprovals.ShouldNotBeEmpty();
        handler.RequestedApprovals[0][0].ToolName.ShouldBe("mcp__server__TestTool");
        invoked.ShouldBeTrue("Tool should have been invoked after approval");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeFunctionAsync_RoutesTheCall_UnderItsOwnConversationId(bool whitelisted)
    {
        // The id the client stamps its metrics with is the id it routes approvals to.
        // One string, one meaning: approvals and metrics cannot name different
        // conversations. Whitelisted tools take the auto-approval notice instead of the
        // approval request, and both carry the same id.
        var handler = new TestApprovalHandler(result: ToolApprovalResult.Approved);
        var function = AIFunctionFactory.Create(() => "result", "mcp__server__TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp__server__TestTool", "call1"));

        var client = new ToolApprovalChatClient(
            fakeClient, handler, "7:9",
            whitelistPatterns: whitelisted ? ["mcp__server__*"] : null);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "test")], new ChatOptions { Tools = [function] });

        handler.ConversationIds.ShouldHaveSingleItem().ShouldBe("7:9");
    }

    [Fact]
    public async Task InvokeFunctionAsync_WhenRejected_TerminatesAndReturnsRejectionMessage()
    {
        // Arrange
        var handler = new TestApprovalHandler(result: ToolApprovalResult.Rejected);
        var invoked = false;
        var function = AIFunctionFactory.Create(() =>
        {
            invoked = true;
            return "result";
        }, "mcp__server__TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp__server__TestTool", "call1"));

        var client = new ToolApprovalChatClient(fakeClient, handler, "conv-test");
        var options = new ChatOptions { Tools = [function] };

        // Act
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        handler.RequestedApprovals.ShouldNotBeEmpty();
        invoked.ShouldBeFalse("Rejected tool should not be invoked");

        var resultContent = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .FirstOrDefault();
        resultContent.ShouldNotBeNull();
        (resultContent.Result?.ToString() ?? string.Empty).ShouldContain("rejected");
    }

    [Fact]
    public async Task InvokeFunctionAsync_WithMultipleTools_OnlyApprovesNonWhitelisted()
    {
        // Arrange
        var handler = new TestApprovalHandler(result: ToolApprovalResult.Approved);
        var whitelistedInvoked = false;
        var nonWhitelistedInvoked = false;

        var whitelistedFunc = AIFunctionFactory.Create(() =>
        {
            whitelistedInvoked = true;
            return "whitelisted result";
        }, "mcp__trusted-server__WhitelistedTool");

        var nonWhitelistedFunc = AIFunctionFactory.Create(() =>
        {
            nonWhitelistedInvoked = true;
            return "non-whitelisted result";
        }, "mcp__untrusted-server__NonWhitelistedTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateMultiToolCallResponse(
            ("mcp__trusted-server__WhitelistedTool", "call1"),
            ("mcp__untrusted-server__NonWhitelistedTool", "call2")));

        var client = new ToolApprovalChatClient(fakeClient, handler, "conv-test", whitelistPatterns: ["mcp__trusted-server__*"]);
        var options = new ChatOptions { Tools = [whitelistedFunc, nonWhitelistedFunc] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        handler.RequestedApprovals.ShouldHaveSingleItem();
        handler.RequestedApprovals[0][0].ToolName.ShouldBe("mcp__untrusted-server__NonWhitelistedTool");
        whitelistedInvoked.ShouldBeTrue();
        nonWhitelistedInvoked.ShouldBeTrue();
    }

    [Fact]
    public async Task InvokeFunctionAsync_AutoApproved_InvokesToolWithoutWaitingForNotify()
    {
        // Arrange
        var notifyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new GatedNotifyApprovalHandler(notifyGate.Task);
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var function = AIFunctionFactory.Create(() =>
        {
            invoked.TrySetResult();
            return "result";
        }, "mcp__server__TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp__server__TestTool", "call1"));
        var client = new ToolApprovalChatClient(fakeClient, handler, "conv-test", whitelistPatterns: ["mcp__server__TestTool"]);
        var options = new ChatOptions { Tools = [function] };

        // Act
        var responseTask = client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert: the tool runs while the auto-approval notification is still in flight
        try
        {
            var completed = await Task.WhenAny(invoked.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.ShouldBe(invoked.Task, "tool invocation must not wait for the notify round trip");
        }
        finally
        {
            notifyGate.TrySetResult();
        }

        await responseTask;
        handler.NotifyCalls.ShouldBe(1);
    }

    private sealed class GatedNotifyApprovalHandler(Task gate) : IToolApprovalHandler
    {
        public int NotifyCalls;

        public Task<ToolApprovalResult> RequestApprovalAsync(
            string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken cancellationToken)
            => Task.FromResult(ToolApprovalResult.Rejected);

        public async Task NotifyAutoApprovedAsync(
            string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref NotifyCalls);
            await gate;
        }
    }
}