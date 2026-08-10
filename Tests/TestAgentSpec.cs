using Infrastructure.Agents;

namespace Tests;

// One default for the construction sites that care about two or three fields. Each test
// varies exactly what it is about with `with`, so what a test depends on is what it names.
internal static class TestAgentSpec
{
    // Every agent gets its own conversation. Thread state is keyed by conversation id and the
    // Redis fixture is shared across a class, so one fixed id made each test in a class append to
    // the history the previous ones left behind — later tests then asked a real model to act on a
    // prompt with several unrelated turns still in context, which is slower and answers wrong.
    // A test that needs two agents to share history says so by naming the id itself.
    public static AgentSpec Default => Named($"conv-test-{Guid.NewGuid():N}");

    public static AgentSpec Named(string conversationId) => new()
    {
        DisplayName = "test-agent",
        Description = "",
        MetricsAgentId = "test-agent",
        RoutingSessionId = $"test-agent:{conversationId}",
        ConversationId = conversationId,
        UserId = "test-user",
        Model = "test-model",
        McpServerEndpoints = [],
        EnabledFeatures = [],
        FilesystemEnabledTools = new HashSet<string>(),
        WhitelistPatterns = [],
        KeepsHistory = true,
        PatchableModelIds = []
    };
}