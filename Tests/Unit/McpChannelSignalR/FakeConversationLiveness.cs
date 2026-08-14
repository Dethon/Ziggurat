using McpChannelSignalR.Attachments;

namespace Tests.Unit.McpChannelSignalR;

// Dead unless a test says otherwise: most tests exercise the sweep on conversations that are
// gone, and the ones about the gate add the conversation here to keep it alive.
public sealed class FakeConversationLiveness : IConversationLiveness
{
    public HashSet<string> Alive { get; } = [];

    public Task<bool> IsAliveAsync(string conversationId, CancellationToken ct) =>
        Task.FromResult(Alive.Contains(conversationId));
}