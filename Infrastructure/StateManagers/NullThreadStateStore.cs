using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.AI;

namespace Infrastructure.StateManagers;

public sealed class NullThreadStateStore : IThreadStateStore
{
    public Task<ChatMessage[]?> GetMessagesAsync(string key) => Task.FromResult<ChatMessage[]?>(null);

    public Task<long> GetMessageCountAsync(string key) => Task.FromResult(0L);

    public Task<ChatMessage[]?> GetTailMessagesAsync(string key, int maxCount) =>
        Task.FromResult<ChatMessage[]?>(null);

    public Task SetMessagesAsync(string key, ChatMessage[] messages) => Task.CompletedTask;

    public Task AppendMessagesAsync(string key, IReadOnlyList<ChatMessage> messages) => Task.CompletedTask;

    public Task DeleteAsync(AgentKey key) => Task.CompletedTask;

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(false);

    public Task SaveTopicAsync(TopicMetadata topic) => Task.CompletedTask;

    public Task DeleteTopicAsync(string agentId, long chatId, string topicId) => Task.CompletedTask;

    public Task<TopicPage> GetTopicPageAsync(string agentId, string spaceSlug, string? cursor, int pageSize)
        => Task.FromResult(new TopicPage([], null));

    public Task MigrateTopicsAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ChatHistoryMessage>> GetHistoryAsync(string agentId, long chatId, long threadId)
        => Task.FromResult<IReadOnlyList<ChatHistoryMessage>>([]);

    public Task<TopicMetadata?> GetTopicByChatIdAndThreadIdAsync(
        string agentId, long chatId, long threadId, CancellationToken ct = default)
        => Task.FromResult<TopicMetadata?>(null);
}