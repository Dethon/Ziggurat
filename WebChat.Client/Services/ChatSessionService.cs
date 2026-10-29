using WebChat.Client.Contracts;
using WebChat.Client.Models;

namespace WebChat.Client.Services;

public sealed class ChatSessionService(IChatLiveConnection liveConnection) : IChatSessionService
{
    public StoredTopic? CurrentTopic { get; private set; }

    public event Action? OnSessionChanged;

    public async Task<HubResult<bool>> StartSessionAsync(StoredTopic topic)
    {
        var result = await liveConnection.InvokeAsync<bool>(
            "StartSession", topic.AgentId, topic.TopicId, topic.ChatId, topic.ThreadId, topic.Name);

        if (result is { IsLive: true, Value: true })
        {
            CurrentTopic = topic;
            OnSessionChanged?.Invoke();
        }

        return result;
    }

    public void ClearSession()
    {
        CurrentTopic = null;
        OnSessionChanged?.Invoke();
    }
}