using Domain.Conversations;
using Domain.DTOs.Channel;

namespace Domain.Contracts;

public interface IConversationFactory
{
    Task<ConversationCreation> CreateAsync(CreateConversationParams p, CancellationToken ct = default);

    // A topic over a conversation another channel already has, so the history the agent writes under
    // that conversation is the one the topic reads. Null when a topic already holds it: a topic is
    // made once per conversation, and showing it again is that topic's business.
    Task<ConversationCreation?> CreateOverAsync(
        ConversationIdentity identity, CreateConversationParams p, CancellationToken ct = default);
}