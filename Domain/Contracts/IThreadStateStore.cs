using Domain.Agents;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IThreadStateStore
{
    Task DeleteAsync(AgentKey key);
    Task<ChatMessage[]?> GetMessagesAsync(string key);
    Task<long> GetMessageCountAsync(string key);
    Task<ChatMessage[]?> GetTailMessagesAsync(string key, int maxCount);
    Task SetMessagesAsync(string key, ChatMessage[] messages);
    Task AppendMessagesAsync(string key, IReadOnlyList<ChatMessage> messages);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync(string agentId, string? spaceSlug = null);
    Task SaveTopicAsync(TopicMetadata topic);
    Task DeleteTopicAsync(string agentId, long chatId, string topicId);

    // The conversation as a reader sees it, which is the same history the agent restores read
    // through a projection. It lives here rather than beside the chat hub so there is one
    // implementation of the key scheme and not two kept in step by hand.
    Task<IReadOnlyList<ChatHistoryMessage>> GetHistoryAsync(string agentId, long chatId, long threadId);

    Task<TopicMetadata?> GetTopicByChatIdAndThreadIdAsync(string agentId, long chatId, long threadId,
        CancellationToken ct = default);
}