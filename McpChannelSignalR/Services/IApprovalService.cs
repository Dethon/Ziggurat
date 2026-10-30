using Domain.DTOs;
using Domain.DTOs.Channel;

namespace McpChannelSignalR.Services;

public interface IApprovalService
{
    Task<string> RequestApprovalAsync(RequestApprovalParams p, Account? asker = null, CancellationToken ct = default);
    Task NotifyAutoApprovedAsync(RequestApprovalParams p);
    Task<string> OfferApprovalAsync(OfferApprovalParams p, CancellationToken ct = default);
    Task SettleApprovalAsync(string approvalId, ToolApprovalResult result);
    Task RespondToApprovalAsync(string approvalId, string result);
}