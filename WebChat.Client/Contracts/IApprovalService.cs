using Domain.DTOs;
using Domain.DTOs.WebChat;

namespace WebChat.Client.Contracts;

public interface IApprovalService
{
    Task<HubResult<bool>> RespondToApprovalAsync(string approvalId, ToolApprovalResult result);
    Task<HubResult<ToolApprovalRequestMessage>> GetPendingApprovalForTopicAsync(string topicId);
    Task<HubResult<IReadOnlyList<ToolApprovalRequestMessage>>> GetOfferedApprovalsAsync();
}