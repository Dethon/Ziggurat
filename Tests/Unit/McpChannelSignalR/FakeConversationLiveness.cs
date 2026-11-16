using Domain.Agents;
using McpChannelSignalR.Attachments;

namespace Tests.Unit.McpChannelSignalR;

// Dead unless a test says otherwise: most tests exercise the sweep on conversations that are
// gone, and the ones about the gate add the conversation here to keep it alive.
public sealed class FakeConversationLiveness : IConversationLiveness
{
    public HashSet<string> Alive { get; } = [];

    // What each sweep asked about, the agent its files named included.
    public List<AgentKey> Asked { get; } = [];

    public Task<bool> IsAliveAsync(AgentKey conversation, CancellationToken ct)
    {
        Asked.Add(conversation);
        return Task.FromResult(Alive.Contains(conversation.ConversationId));
    }
}