using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;
using WebChat.Client.State.Hub;

namespace WebChat.Client.Services;

public sealed class HubEventBinder(IHubEventDispatcher hubEventDispatcher) : IHubEventBinder
{
    private readonly List<IDisposable> _registrations = [];

    public void Bind(IChatHubConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _registrations.AddRange(
        [
            connection.On<TopicChangedNotification>(
                "OnTopicChanged", hubEventDispatcher.HandleTopicChanged),
            connection.On<StreamStartedNotification>(
                "OnStreamStarted", hubEventDispatcher.HandleStreamStarted),
            connection.On<ApprovalResolvedNotification>(
                "OnApprovalResolved", hubEventDispatcher.HandleApprovalResolved),
            connection.On<ApprovalOfferedNotification>(
                "OnApprovalOffered", hubEventDispatcher.HandleApprovalOffered),
            connection.On<AlertOfferedNotification>(
                "OnAlertOffered", hubEventDispatcher.HandleAlertOffered),
            connection.On<AlertSettledNotification>(
                "OnAlertSettled", hubEventDispatcher.HandleAlertSettled),
            connection.On<UserMessageNotification>(
                "OnUserMessage", hubEventDispatcher.HandleUserMessage),
            connection.On<AgentsUpdatedNotification>(
                "OnAgentsUpdated", hubEventDispatcher.HandleAgentsUpdated)
        ]);
    }

    public void Unbind()
    {
        _registrations.ForEach(registration => registration.Dispose());
        _registrations.Clear();
    }
}