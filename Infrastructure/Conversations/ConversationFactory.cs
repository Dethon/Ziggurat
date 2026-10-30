using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;

namespace Infrastructure.Conversations;

public sealed class ConversationFactory(IThreadStateStore store, TimeProvider time) : IConversationFactory
{
    public async Task<ConversationCreation> CreateAsync(CreateConversationParams p, CancellationToken ct = default)
    {
        var topicId = ConversationIdGenerator.NewTopicId();
        return await SaveAsync(topicId, ConversationIdGenerator.CreateFor(topicId), p);
    }

    public async Task<ConversationCreation?> CreateOverAsync(
        ConversationIdentity identity, CreateConversationParams p, CancellationToken ct = default)
    {
        var held = await store.GetTopicByChatIdAndThreadIdAsync(p.AgentId, identity.ChatId, identity.ThreadId, ct);
        return held is not null
            ? null
            : await SaveAsync(ConversationIdGenerator.NewTopicId(), identity, p);
    }

    private async Task<ConversationCreation> SaveAsync(
        string topicId, ConversationIdentity identity, CreateConversationParams p)
    {
        // A group's conversation is everyone's in it, so nobody owns its topic and the household
        // shares it, though the turn that opened it acts for its sender. The household is still
        // recorded: it is the one the group is bound to, which the sender's must be to get a turn.
        var account = p.Principal?.Account;
        var owner = p.Group ? null : account;
        var topic = new TopicMetadata(
            topicId,
            identity.ChatId,
            identity.ThreadId,
            p.AgentId,
            p.TopicName,
            time.GetUtcNow(),
            LastMessageAt: null,
            SpaceSlug: "default",
            OwnerId: owner?.UserId,
            TenantId: account?.TenantId,
            Shared: owner is null,
            OwnerName: owner is null ? null : p.Sender);

        await store.SaveTopicAsync(topic);
        return new ConversationCreation(identity, topic);
    }
}