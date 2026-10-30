using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Hub;

public interface IHubEventDispatcher
{
    void HandleTopicChanged(TopicChangedNotification notification);
    void HandleStreamStarted(StreamStartedNotification notification);
    void HandleApprovalResolved(ApprovalResolvedNotification notification);
    void HandleApprovalOffered(ApprovalOfferedNotification notification);
    void HandleUserMessage(UserMessageNotification notification);
    void HandleAgentsUpdated(IReadOnlyList<AgentCatalogEntry> agents);
}