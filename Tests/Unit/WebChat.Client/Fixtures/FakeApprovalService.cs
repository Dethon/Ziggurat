using Domain.DTOs;
using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeApprovalService : IApprovalService
{
    private readonly Dictionary<string, ToolApprovalRequestMessage> _pendingApprovals = new();
    private readonly List<(string ApprovalId, ToolApprovalResult Result)> _responses = new();

    public void SetPendingApproval(string topicId, ToolApprovalRequestMessage approval)
    {
        _pendingApprovals[topicId] = approval;
    }

    public void ClearPendingApproval(string topicId)
    {
        _pendingApprovals.Remove(topicId);
    }

    public IReadOnlyList<(string ApprovalId, ToolApprovalResult Result)> Responses => _responses;

    // Set to answer not live for every call, the way a transport between connections does.
    public bool NotLive { get; set; }

    public Task<HubResult<bool>> RespondToApprovalAsync(string approvalId, ToolApprovalResult result)
    {
        if (NotLive)
        {
            return Task.FromResult(HubResult<bool>.NotLive);
        }

        _responses.Add((approvalId, result));
        return Task.FromResult(HubResult<bool>.Answered(true));
    }

    public List<ToolApprovalRequestMessage> Offered { get; } = [];

    public Task<HubResult<IReadOnlyList<ToolApprovalRequestMessage>>> GetOfferedApprovalsAsync() =>
        Task.FromResult(NotLive
            ? HubResult<IReadOnlyList<ToolApprovalRequestMessage>>.NotLive
            : HubResult<IReadOnlyList<ToolApprovalRequestMessage>>.Answered([.. Offered]));

    public Task<HubResult<ToolApprovalRequestMessage>> GetPendingApprovalForTopicAsync(string topicId)
    {
        return Task.FromResult(NotLive
            ? HubResult<ToolApprovalRequestMessage>.NotLive
            : HubResult<ToolApprovalRequestMessage>.Answered(
                _pendingApprovals.GetValueOrDefault(topicId)));
    }
}