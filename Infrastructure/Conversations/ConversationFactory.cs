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
        var owner = p.Principal?.Account;
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
            TenantId: owner?.TenantId,
            Shared: owner is null,
            OwnerName: owner is null ? null : p.Sender);

        await store.SaveTopicAsync(topic);
        return new ConversationCreation(identity, topic);
    }
}