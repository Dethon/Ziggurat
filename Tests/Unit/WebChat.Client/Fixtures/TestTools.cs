using Domain.DTOs.WebChat;

namespace Tests.Unit.WebChat.Client.Fixtures;

// A tool call and its answer as the hub streams them: two steps under one call id.
public static class TestTools
{
    public static ToolCallStep Call(string callId, string name = "mcp__x__search") =>
        new() { CallId = callId, Name = name, Arguments = """{"q":"query"}""" };

    public static ToolCallStep Answer(string callId, string result = "found") =>
        new() { CallId = callId, Result = result, Outcome = ToolCallOutcome.Done };

    public static IReadOnlyList<ToolCallStep> Calls(params string[] callIds) => [.. callIds.Select(id => Call(id))];
}