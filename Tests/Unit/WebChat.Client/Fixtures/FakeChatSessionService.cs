using WebChat.Client.Contracts;
using WebChat.Client.Models;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeChatSessionService(CallRecorder? recorder = null) : IChatSessionService
{
    public StoredTopic? CurrentTopic { get; private set; }

    public event Action? OnSessionChanged;

    public IReadOnlyList<string> RegisteredUserIds => _registeredUserIds;

    private readonly List<string> _registeredUserIds = [];

    // Set to answer not live for every call, the way a transport between connections does.
    public bool NotLive { get; set; }

    public bool SessionRefused { get; set; }

    public Task<HubResult<bool>> StartSessionAsync(StoredTopic topic)
    {
        recorder?.Record($"start-session:{topic.TopicId}");

        if (NotLive)
        {
            return Task.FromResult(HubResult<bool>.NotLive);
        }

        if (SessionRefused)
        {
            return Task.FromResult(HubResult<bool>.Answered(false));
        }

        CurrentTopic = topic;
        OnSessionChanged?.Invoke();
        return Task.FromResult(HubResult<bool>.Answered(true));
    }

    // When set, registration is answered only once the test completes it: the window in which a
    // call made after registering is still waiting on the server.
    public TaskCompletionSource? RegistrationAnswer { get; set; }

    public async Task<HubResult<Nothing>> RegisterUserAsync(string userId)
    {
        recorder?.Record("register-user");

        if (RegistrationAnswer is { } answer)
        {
            await answer.Task;
        }

        if (NotLive)
        {
            return HubResult<Nothing>.NotLive;
        }

        _registeredUserIds.Add(userId);
        return HubResult<Nothing>.Answered(default);
    }

    public void ClearSession()
    {
        CurrentTopic = null;
        OnSessionChanged?.Invoke();
    }
}