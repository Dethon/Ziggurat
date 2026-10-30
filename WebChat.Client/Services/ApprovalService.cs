using Domain.DTOs;
using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class ApprovalService(IChatLiveConnection liveConnection) : IApprovalService
{
    public Task<HubResult<bool>> RespondToApprovalAsync(string approvalId, ToolApprovalResult result) =>
        liveConnection.InvokeAsync<bool>("RespondToApprovalAsync", approvalId, result);

    public Task<HubResult<ToolApprovalRequestMessage>> GetPendingApprovalForTopicAsync(string topicId) =>
        liveConnection.InvokeAsync<ToolApprovalRequestMessage>("GetPendingApprovalForTopic", topicId);

    public Task<HubResult<IReadOnlyList<ToolApprovalRequestMessage>>> GetOfferedApprovalsAsync() =>
        liveConnection.InvokeAsync<IReadOnlyList<ToolApprovalRequestMessage>>("GetOfferedApprovals");
}