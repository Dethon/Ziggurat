using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Shouldly;
using Tests.Eval.Harness;
using Tests.Unit.Infrastructure.Helpers;
using static Tests.Unit.Infrastructure.Helpers.ToolApprovalResponseFactory;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// The observer seam, proven against a scripted chat client replaying canned tool-call sequences:
// deterministic, no network, no model. What it has to capture is everything an eval assertion
// reads — including the two calls that never produce a result, the one whose tool threw and the
// one naming a tool that does not exist.
public class ToolInvocationObserverTests
{
    [Fact]
    public async Task WithNoObserver_TheTurnStillApprovesAndInvokes()
    {
        var handler = new TestApprovalHandler(ToolApprovalResult.Approved);
        var invoked = false;
        var function = AIFunctionFactory.Create(() =>
        {
            invoked = true;
            return "result";
        }, "mcp__server__TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp__server__TestTool", "call1"));

        var client = new ToolApprovalChatClient(fakeClient, handler, "conv");

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "test")], new ChatOptions { Tools = [function] });

        handler.RequestedApprovals.ShouldHaveSingleItem();
        invoked.ShouldBeTrue();
    }

    [Fact]
    public async Task EveryInvocation_IsRecordedOnce_InOrder_WithItsRawArguments()
    {
        var recording = new Recording();
        var first = AIFunctionFactory.Create((string path) => $"read {path}", "domain__filesystem_read");
        var second = AIFunctionFactory.Create((string path) => $"wrote {path}", "domain__filesystem_create");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse(
            "domain__filesystem_read", "call1", new Dictionary<string, object?> { ["path"] = "/timers" }));
        fakeClient.SetNextResponse(CreateToolCallResponse(
            "domain__filesystem_create", "call2", new Dictionary<string, object?> { ["path"] = "/timers/pasta" }));

        var client = Approving(fakeClient, recording);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "test")], new ChatOptions { Tools = [first, second] });

        recording.Calls.Select(c => c.ToolName)
            .ShouldBe(["domain__filesystem_read", "domain__filesystem_create"]);
        recording.Calls.Select(c => c.Sequence).ShouldBe([0, 1]);
        JsonDocument.Parse(recording.Calls[1].Arguments).RootElement
            .GetProperty("path").GetString().ShouldBe("/timers/pasta");
        recording.Calls[0].Outcome.ShouldBe(ToolInvocationOutcome.Completed);
        recording.Calls[0].Result.ShouldNotBeNull().ShouldContain("read /timers");
    }

    [Fact]
    public async Task AToolThatThrows_IsRecordedWithItsError_AndTheRecordingContinues()
    {
        var recording = new Recording();
        var thrower = AIFunctionFactory.Create(
            string () => throw new InvalidOperationException("mount is read-only"),
            "domain__filesystem_create");
        var survivor = AIFunctionFactory.Create(() => "listed", "domain__filesystem_glob");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("domain__filesystem_create", "call1"));
        fakeClient.SetNextResponse(CreateToolCallResponse("domain__filesystem_glob", "call2"));

        var client = Approving(fakeClient, recording);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "test")], new ChatOptions { Tools = [thrower, survivor] });

        var failed = recording.Calls[0];
        failed.ToolName.ShouldBe("domain__filesystem_create");
        failed.Outcome.ShouldBe(ToolInvocationOutcome.Failed);
        failed.Error.ShouldNotBeNull().ShouldContain("mount is read-only");
        recording.Calls.Select(c => c.ToolName).ShouldContain("domain__filesystem_glob");
    }

    [Fact]
    public async Task ACallNamingAToolThatDoesNotExist_IsRecorded()
    {
        var recording = new Recording();
        var known = AIFunctionFactory.Create(() => "ok", "domain__filesystem_glob");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp__mcp-timers__set_timer", "call1"));

        var client = Approving(fakeClient, recording);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "test")], new ChatOptions { Tools = [known] });

        var call = recording.Calls.ShouldHaveSingleItem();
        call.ToolName.ShouldBe("mcp__mcp-timers__set_timer");
        call.Outcome.ShouldBe(ToolInvocationOutcome.NotFound);
    }

    [Fact]
    public async Task ConcurrentInvocations_AreAllRecorded_EachAtItsOwnPosition()
    {
        var recording = new Recording();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = AIFunctionFactory.Create(async () =>
        {
            await gate.Task;
            return "slow";
        }, "domain__filesystem_read");
        var fast = AIFunctionFactory.Create(() =>
        {
            gate.TrySetResult();
            return "fast";
        }, "domain__filesystem_glob");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateMultiToolCallResponse(
            ("domain__filesystem_read", "call1"), ("domain__filesystem_glob", "call2")));

        var client = Approving(fakeClient, recording);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "test")], new ChatOptions { Tools = [slow, fast] });

        recording.Calls.Count.ShouldBe(2);
        recording.Calls.Select(c => c.Sequence).Distinct().Count().ShouldBe(2);
        recording.Calls.Select(c => c.ToolName)
            .ShouldBe(["domain__filesystem_read", "domain__filesystem_glob"]);
    }

    private static ToolApprovalChatClient Approving(IChatClient inner, IToolInvocationObserver observer) =>
        new(inner, new TestApprovalHandler(ToolApprovalResult.Approved), "conv",
            whitelistPatterns: ["domain__*", "mcp__*"], observer: observer);
}