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

    // One page of the ordinary list, most recently written first. There is no call that returns
    // every topic: the list is read one way only, so a new caller cannot reintroduce a fetch
    // whose cost grows with everything the space has ever had. The cursor is where the last page
    // ended, so reaching an old conversation costs what reaching a recent one costs.
    // Archived reads the same index below the archive cutoff. Nothing marks a topic archived and
    // nothing unmarks it — see ADR 0024.
    Task<TopicPage> GetTopicPageAsync(
        string agentId, string spaceSlug, string? cursor, int pageSize, bool archived = false);

    // By what a conversation is called and by what was said in it, over both ranges at once —
    // search is the way into the archive, so a cutoff has no business in it. Paged like any
    // other list of topics.
    Task<TopicPage> SearchTopicsAsync(
        string agentId, string spaceSlug, string query, string? cursor, int pageSize);

    // keepTtl is for writes that are not the conversation's own: a rename must not push the purge
    // horizon back, or search returns a topic whose transcript expired months before its record.
    Task SaveTopicAsync(TopicMetadata topic, bool keepTtl = false);
    Task DeleteTopicAsync(string agentId, long chatId, string topicId);

    // Moves the topic's read position up to what it currently holds. Asked of the store rather
    // than told by a caller, so a message that landed while the reader was reading is not
    // counted unread.
    Task MarkTopicReadAsync(string agentId, long chatId, string topicId);

    // The conversation as a reader sees it, which is the same history the agent restores read
    // through a projection. It lives here rather than beside the chat hub so there is one
    // implementation of the key scheme and not two kept in step by hand.
    Task<IReadOnlyList<ChatHistoryMessage>> GetHistoryAsync(string agentId, long chatId, long threadId);

    Task<TopicMetadata?> GetTopicByChatIdAndThreadIdAsync(string agentId, long chatId, long threadId,
        CancellationToken ct = default);

    // The stored topic a caller already names. A writer holding a client's copy reads this first,
    // so counters the store learned while the browser wasn't looking are never written back stale.
    Task<TopicMetadata?> GetTopicAsync(string agentId, long chatId, string topicId);
}