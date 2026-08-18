using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Tests.Unit.Infrastructure.Helpers;

namespace Tests.Eval.Harness;

// A canned tool-call sequence played through the real approval client, producing a real
// recording. This is the only legitimate use of a scripted client in this suite: it tests the
// harness, never the prompts — a scenario driven this way would script the decision it then
// asserts, which is the whole reason ADR-0030 puts a real model behind the scenarios.
public static class ScriptedTurn
{
    public sealed record Step(string Tool, IDictionary<string, object?>? Arguments = null, string Result = "ok");

    public static Step Call(string tool, object? path = null, string result = "ok") =>
        new(tool, path is null ? null : new Dictionary<string, object?> { ["path"] = path }, result);

    // One tool call per iteration, in the order given, then a final assistant message. Concurrency
    // is not modelled here: what the ordering check is about is the order calls were issued in,
    // and a scripted client that issued two at once would be testing the seam, not the check.
    public static async Task<Recording> RunAsync(string reply, params Step[] steps)
    {
        var recording = new Recording();
        var client = new FakeChatClient { Route = new ServedRoute("scripted/model", "Scripted") };

        var tools = steps
            .DistinctBy(s => s.Tool)
            .Select(step => (AITool)AIFunctionFactory.Create(
                (string? path) => steps.First(s => s.Tool == step.Tool).Result, step.Tool))
            .ToList();

        steps
            .Select((step, index) => ToolApprovalResponseFactory.CreateToolCallResponse(
                step.Tool, $"call{index}", step.Arguments))
            .ToList()
            .ForEach(client.SetNextResponse);

        client.SetNextResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, reply)])
        {
            FinishReason = ChatFinishReason.Stop
        });

        using var approving = new ToolApprovalChatClient(
            client, new TestApprovalHandler(ToolApprovalResult.Approved), "eval-scripted",
            whitelistPatterns: ["*"], observer: recording);

        var response = await approving.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "scripted")],
            new ChatOptions { Tools = tools, Instructions = "scripted system prompt" });

        recording.Reply = response.Text;
        return recording;
    }
}