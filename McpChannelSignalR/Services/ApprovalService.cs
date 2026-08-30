using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelSignalR.Internal;

namespace McpChannelSignalR.Services;

public sealed class ApprovalService(
    StreamService streamService,
    SessionService sessionService,
    IHubNotificationSender hubNotificationSender,
    ILogger<ApprovalService> logger) : IApprovalService
{
    private readonly ConcurrentDictionary<string, ApprovalContext> _pendingApprovals = new();

    public async Task<string> RequestApprovalAsync(RequestApprovalParams p)
    {
        // A question nobody can see must not hold the turn (ADR-0035). With no live session the
        // prompt renders to nobody, and a registration under the unresolved spelling could never
        // be answered — not even deleting the topic released it. Deny at once, naming the reason,
        // so the run continues and the persisted reply explains itself.
        var topicId = sessionService.GetTopicIdByConversationId(p.ConversationId);
        if (topicId is null)
        {
            logger.LogDebug(
                "RequestApproval: no live session for conversation {ConversationId}; denying immediately",
                p.ConversationId);
            return "rejected: no live session to approve in";
        }

        var requests = p.Requests;
        var approvalId = Guid.NewGuid().ToString("N")[..8];

        var context = new ApprovalContext
        {
            TopicId = topicId,
            Requests = requests
        };

        _pendingApprovals[approvalId] = context;

        try
        {
            var approvalMessage = new ChatStreamMessage
            {
                ApprovalRequest = new ToolApprovalRequestMessage(approvalId, requests)
            };

            await streamService.WriteMessageAsync(topicId, approvalMessage);

            var result = await context.WaitForApprovalAsync(CancellationToken.None);

            if (result is not (ToolApprovalResult.Approved or ToolApprovalResult.ApprovedAndRemember))
            {
                return result.ToString().ToLowerInvariant();
            }

            await WriteToolCallsAsync(topicId, requests);

            return result.ToString().ToLowerInvariant();
        }
        finally
        {
            _pendingApprovals.TryRemove(approvalId, out _);
            context.Dispose();
        }
    }

    public Task NotifyAutoApprovedAsync(RequestApprovalParams p)
    {
        // A notification nobody can see costs nothing and blocks nothing (ADR-0035): the tool
        // calls it would render are in the persisted reply already.
        var topicId = sessionService.GetTopicIdByConversationId(p.ConversationId);
        if (topicId is null)
        {
            logger.LogDebug(
                "NotifyAutoApproved: no live session for conversation {ConversationId}; nothing to render to",
                p.ConversationId);
            return Task.CompletedTask;
        }

        return WriteToolCallsAsync(topicId, p.Requests);
    }

    // The one route a tool call reaches the browser by. It is buffered with the rest of the reply,
    // so a reload replays it and it arrives in order; a hub push beside it would be the same text a
    // second time, reaching no browser this does not. Grouped by message id because that is what
    // says which bubble the call belongs to — unlabelled, it lands on whatever is being written.
    private async Task WriteToolCallsAsync(string topicId, IReadOnlyList<ToolApprovalRequest> requests)
    {
        var messages = requests
            .GroupBy(request => request.MessageId)
            .Select(g => new ChatStreamMessage
            {
                MessageId = g.Key,
                ToolCalls = FormatToolCalls(g.ToArray())
            });

        foreach (var message in messages)
        {
            await streamService.WriteMessageAsync(topicId, message);
        }
    }

    public async Task RespondToApprovalAsync(string approvalId, string result)
    {
        if (!_pendingApprovals.TryRemove(approvalId, out var context))
        {
            logger.LogWarning("RespondToApproval: approvalId {ApprovalId} not found or already processed", approvalId);
            return;
        }

        var approvalResult = Enum.Parse<ToolApprovalResult>(result, ignoreCase: true);

        await NotifyResolvedAsync(context.TopicId, approvalId);

        context.TrySetResult(approvalResult);
        context.Dispose();
    }

    // Taking the prompt off every browser showing it is the whole of this push. What an approval
    // let through is written into the topic's stream, once, by RequestApprovalAsync.
    private async Task NotifyResolvedAsync(string topicId, string approvalId)
    {
        sessionService.TryGetSession(topicId, out var session);

        try
        {
            var notification = new ApprovalResolvedNotification(
                topicId, approvalId, SpaceSlug: session?.SpaceSlug);
            await SendToSpaceOrAllAsync(session?.SpaceSlug, "OnApprovalResolved", notification);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to notify approval resolved for topic {TopicId}", topicId);
        }
    }

    public bool IsApprovalPending(string approvalId)
    {
        return _pendingApprovals.ContainsKey(approvalId);
    }

    public ToolApprovalRequestMessage? GetPendingApprovalForTopic(string topicId)
    {
        var pending = _pendingApprovals
            .FirstOrDefault(kv => kv.Value.TopicId == topicId);

        return pending.Key is null
            ? null
            : new ToolApprovalRequestMessage(pending.Key, pending.Value.Requests);
    }

    // The topic is over — cancelled or deleted — so every prompt it raised is over with it. The
    // waiting tool call is told no, and the browsers showing the prompt are told the same way an
    // answer tells them: left up, the modal asks about a turn that no longer exists.
    public async Task CancelPendingApprovalsForTopicAsync(string topicId)
    {
        var expiredApprovals = _pendingApprovals
            .Where(kv => kv.Value.TopicId == topicId)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var approvalId in expiredApprovals)
        {
            if (!_pendingApprovals.TryRemove(approvalId, out var context))
            {
                continue;
            }

            await NotifyResolvedAsync(topicId, approvalId);

            context.TrySetResult(ToolApprovalResult.Rejected);
            context.Dispose();
        }
    }

    private async Task SendToSpaceOrAllAsync(string? spaceSlug, string methodName, object notification)
    {
        if (spaceSlug is not null)
        {
            await hubNotificationSender.SendToGroupAsync($"space:{spaceSlug}", methodName, notification);
        }
        else
        {
            await hubNotificationSender.SendAsync(methodName, notification);
        }
    }

    private static string FormatToolCalls(IReadOnlyList<ToolApprovalRequest> requests)
    {
        var sb = new StringBuilder();

        foreach (var request in requests)
        {
            var toolName = request.ToolName.Split("__").Last();
            sb.AppendLine($"🔧 {toolName}");

            if (request.Arguments.Count <= 0)
            {
                continue;
            }

            foreach (var (key, value) in request.Arguments)
            {
                var formattedValue = FormatArgumentValue(value);
                if (formattedValue.Length > 100)
                {
                    formattedValue = formattedValue[..100] + "...";
                }

                sb.AppendLine($"  {key}: {formattedValue}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatArgumentValue(object? value)
    {
        return value switch
        {
            null => "null",
            string s => s.Replace("\n", " ").Replace("\r", ""),
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString()?.Replace("\n", " ") ?? "",
            JsonElement je => je.GetRawText(),
            _ => value.ToString() ?? ""
        };
    }
}