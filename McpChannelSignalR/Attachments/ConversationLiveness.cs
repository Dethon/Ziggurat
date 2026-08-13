using Domain.Agents;
using Domain.Contracts;

namespace McpChannelSignalR.Attachments;

// Whether a conversation still exists in the store the transcripts live in. The sweep asks so a
// topic and its files die together: the conversation's purge clock restarts on every append, and a
// file swept twelve months after upload while its topic is still in use dies eleven months early
// by the topic's own reckoning.
public interface IConversationLiveness
{
    Task<bool> IsAliveAsync(string conversationId, CancellationToken ct);
}

// An upload is scoped to a topic, never to an agent, so the store cannot spell the one history key
// a conversation has — aliveness is any registered agent still holding it. An empty catalog
// answers alive: right after a restart nothing has registered yet, and deleting on ignorance is
// the one wrong the sweep must not do; the files go on the next tick instead.
public sealed class AgentConversationLiveness(IAgentCatalog catalog, IThreadStateStore store)
    : IConversationLiveness
{
    public async Task<bool> IsAliveAsync(string conversationId, CancellationToken ct)
    {
        var agents = catalog.GetAll();
        if (agents.Count == 0)
        {
            return true;
        }

        foreach (var agent in agents)
        {
            if (await store.ExistsAsync(new AgentKey(conversationId, agent.Id).ToString(), ct))
            {
                return true;
            }
        }

        return false;
    }
}