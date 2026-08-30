using System.Collections.Concurrent;
using Domain.Conversations;
using Domain.DTOs.Channel;
using McpChannelSignalR.Internal;

namespace McpChannelSignalR.Services;

public sealed class SessionService : ISessionService
{
    private readonly ConcurrentDictionary<string, ChannelSession> _sessions = new();
    private readonly ConcurrentDictionary<long, string> _chatToTopic = new();
    private readonly ConcurrentDictionary<string, string> _conversationToTopic = new();

    public Task<string> CreateConversationAsync(CreateConversationParams p)
    {
        var topicId = ConversationIdGenerator.NewTopicId();
        var identity = ConversationIdGenerator.CreateFor(topicId);
        StartSession(topicId, p.AgentId, identity.ChatId, identity.ThreadId, spaceSlug: "default", topicName: p.TopicName);
        return Task.FromResult(identity.ConversationId);
    }

    public bool StartSession(string topicId, string agentId, long chatId, long threadId, string? spaceSlug = null, string? topicName = null)
    {
        var session = new ChannelSession(agentId, chatId, threadId, spaceSlug, topicName);
        _sessions[topicId] = session;
        _chatToTopic[chatId] = topicId;
        _conversationToTopic[session.Identity.ConversationId] = topicId;
        return true;
    }

    public bool TryGetSession(string topicId, out ChannelSession? session)
    {
        return _sessions.TryGetValue(topicId, out session);
    }

    public void EndSession(string topicId)
    {
        if (!_sessions.TryRemove(topicId, out var session))
        {
            return;
        }

        _chatToTopic.TryRemove(session.ChatId, out _);
        _conversationToTopic.TryRemove(session.Identity.ConversationId, out _);
    }

    public string? GetTopicIdByChatId(long chatId)
    {
        return _chatToTopic.GetValueOrDefault(chatId);
    }

    public string? GetTopicIdByConversationId(string conversationId)
    {
        return _conversationToTopic.GetValueOrDefault(conversationId);
    }

    public ChannelSession? GetSessionByConversationId(string conversationId)
    {
        var topicId = GetTopicIdByConversationId(conversationId);
        return topicId is not null && _sessions.TryGetValue(topicId, out var session) ? session : null;
    }

}