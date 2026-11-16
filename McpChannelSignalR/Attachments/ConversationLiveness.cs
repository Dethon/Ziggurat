using Domain.Agents;
using Domain.Contracts;

namespace McpChannelSignalR.Attachments;

// Whether a conversation still exists in the store the transcripts live in. The sweep asks so a
// topic and its files die together: the conversation's purge clock restarts on every append, and a
// file swept twelve months after upload while its topic is still in use dies eleven months early
// by the topic's own reckoning.
public interface IConversationLiveness
{
    Task<bool> IsAliveAsync(AgentKey conversation, CancellationToken ct);
}

// A file names the agent whose conversation it was kept for, so aliveness is that one history:
// the catalogue the agent registers here is the home's agents, no list of a household's, and a
// household's own agent may go by an id the home lacks. A file kept before files named their agent
// was the home's, as every conversation then was, so it is alive while any registered agent still
// holds it — and an empty catalogue answers alive: right after a restart nothing has registered
// yet, and deleting on ignorance is the one wrong the sweep must not do; the files go on the next
// tick instead.
public sealed class AgentConversationLiveness(IAgentCatalog home, IThreadStateStore store)
    : IConversationLiveness
{
    public async Task<bool> IsAliveAsync(AgentKey conversation, CancellationToken ct)
    {
        if (conversation.AgentId is not null)
        {
            return await store.ExistsAsync(conversation.ToString(), ct);
        }

        var agents = home.GetAll();
        if (agents.Count == 0)
        {
            return true;
        }

        foreach (var agent in agents)
        {
            if (await store.ExistsAsync(new AgentKey(conversation.ConversationId, agent.Id).ToString(), ct))
            {
                return true;
            }
        }

        return false;
    }
}