namespace Domain.DTOs.Channel;

// The agent's internal route that mints a topic for a conversation a channel server opens on its
// own — voice's, whose satellites start conversations nobody else anchors. The agent owns topics:
// it writes the one the chat client reads, so the server asking holds no topic store of its own.
// Who the topic is for is never in the body: it is whoever the accounts service's assertion,
// carried in X-Ziggurat-Assertion, names, so a caller cannot mint a topic for anyone else.
public static class TopicMinting
{
    // Under /internal like the agent's keys, which no proxy route reaches on any host.
    public const string Path = "/internal/topics";
}

public sealed record TopicMintRequest(string AgentId, string TopicName, string Sender);

// The topic and the conversation it holds, whose id the asking channel sends its turns under.
public sealed record TopicMinted(string TopicId, string ConversationId);