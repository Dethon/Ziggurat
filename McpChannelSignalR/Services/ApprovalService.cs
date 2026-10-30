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
    IThreadStateStore threadStore,
    IHubNotificationSender hubNotificationSender,
    IPushNotificationService pushNotifications,
    AccountConnections connections,
    ILogger<ApprovalService> logger) : IApprovalService
{
    private readonly ConcurrentDictionary<string, ApprovalContext> _pendingApprovals = new();

    public async Task<string> RequestApprovalAsync(
        RequestApprovalParams p, Account? asker = null, CancellationToken ct = default)
    {
        // A question nobody can see must not hold the turn (ADR-0035). With no live session the
        // prompt renders to nobody, and a registration under the unresolved spelling could never
        // be answered — not even deleting the topic released it. It is no answer at once, so the
        // run continues — or, when its asker is offered it elsewhere, waits on them there.
        var topicId = sessionService.GetTopicIdByConversationId(p.ConversationId);
        if (topicId is null)
        {
            logger.LogDebug(
                "RequestApproval: no live session for conversation {ConversationId}; not asked here",
                p.ConversationId);
            return ChannelProtocol.NoAnswer;
        }

        var requests = p.Requests;
        var approvalId = OwnIdOf(p.ApprovalId);

        var context = new ApprovalContext
        {
            TopicId = topicId,
            Requests = requests,
            Asker = asker
        };

        _pendingApprovals[approvalId] = context;

        try
        {
            var approvalMessage = new ChatStreamMessage
            {
                ApprovalRequest = new ToolApprovalRequestMessage(approvalId, requests, asker?.UserId)
            };

            await streamService.WriteMessageAsync(topicId, approvalMessage);

            var (result, _) = await WaitAsync(approvalId, context, ct);

            // Answered here or on another of the asker's channels, what was approved is let through
            // here the same way, and the topic shows the call.
            if (result is ToolApprovalResult.Approved or ToolApprovalResult.ApprovedAndRemember)
            {
                await WriteToolCallsAsync(topicId, requests);
            }

            return result.ToString().ToLowerInvariant();
        }
        finally
        {
            _pendingApprovals.TryRemove(approvalId, out _);
            context.Dispose();
        }
    }

    // The question another channel asked, offered to its asker on every chat client they have open,
    // wherever they are — or, with none open, on their devices, waiting for them to open one. It
    // needs no live session: it is put to a person, not to a topic.
    public async Task<string> OfferApprovalAsync(OfferApprovalParams p, CancellationToken ct = default)
    {
        var approvalId = OwnIdOf(p.ApprovalId);
        var session = sessionService.GetSessionByConversationId(p.ConversationId);
        var context = new ApprovalContext
        {
            TopicId = sessionService.GetTopicIdByConversationId(p.ConversationId),
            SpaceSlug = session?.SpaceSlug,
            Requests = p.Requests,
            Asker = p.Asker,
            Offered = true
        };

        if (!_pendingApprovals.TryAdd(approvalId, context))
        {
            // Already showing here, asked in a topic: one card is enough.
            return ChannelProtocol.NoAnswer;
        }

        try
        {
            var request = new ToolApprovalRequestMessage(approvalId, p.Requests, p.Asker.UserId);
            await hubNotificationSender.SendToGroupAsync(
                TopicAudience.Account(p.Asker.UserId), "OnApprovalOffered",
                new ApprovalOfferedNotification(request, context.TopicId), CancellationToken.None);
            if (!connections.AnyOpen(p.Asker.UserId))
            {
                await PushOfferAsync(p, context.SpaceSlug ?? SpaceConfig.DefaultSlug);
            }

            var (result, elsewhere) = await WaitAsync(approvalId, context, ct);
            return elsewhere ? ChannelProtocol.NoAnswer : result.ToString().ToLowerInvariant();
        }
        finally
        {
            _pendingApprovals.TryRemove(approvalId, out _);
            context.Dispose();
        }
    }

    // Called off — the turn stopped, or the question was answered elsewhere before a settle could
    // name it here — the card is taken down as an answer would take it down.
    private async Task<(ToolApprovalResult Result, bool Elsewhere)> WaitAsync(
        string approvalId, ApprovalContext context, CancellationToken ct)
    {
        try
        {
            return await context.WaitForApprovalAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (_pendingApprovals.TryRemove(approvalId, out _))
            {
                await NotifyResolvedAsync(context, approvalId, answeredElsewhere: false);
            }

            throw;
        }
    }

    // The question's own id when the agent named one, so it can be settled by it; a whole GUID
    // either way, since the id is all a browser sends to answer it.
    private static string OwnIdOf(string? approvalId) =>
        approvalId is not null && Guid.TryParseExact(approvalId, "N", out _)
            ? approvalId
            : Guid.NewGuid().ToString("N");

    private async Task PushOfferAsync(OfferApprovalParams p, string spaceSlug)
    {
        try
        {
            var tools = string.Join(", ", p.Requests.Select(r => r.ToolName.Split("__").Last()));
            await pushNotifications.SendToPersonAsync(
                p.Asker.UserId, spaceSlug, "Approval needed", $"Approve {tools}?", "/");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to push approval {ApprovalId} to its asker's devices", p.ApprovalId);
        }
    }

    // Answered on another of the asker's channels. A question asked here still lets through what it
    // asked about, as if answered on the card; every browser showing it is told who answered it.
    public async Task SettleApprovalAsync(string approvalId, ToolApprovalResult result)
    {
        if (!_pendingApprovals.TryRemove(approvalId, out var context))
        {
            return;
        }

        await NotifyResolvedAsync(context, approvalId, answeredElsewhere: true);
        context.TrySetResult(result, elsewhere: true);
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

        await NotifyResolvedAsync(context, approvalId, answeredElsewhere: false);

        context.TrySetResult(approvalResult);
    }

    // Taking the prompt off every browser showing it is the whole of this push, so it goes to the
    // browsers that may see the topic and no others — or, for a question offered to a person, to
    // that person's. What an approval let through is written into the topic's stream, once, by
    // RequestApprovalAsync.
    private async Task NotifyResolvedAsync(ApprovalContext context, string approvalId, bool answeredElsewhere)
    {
        if (context is { Offered: true, Asker: { } asker })
        {
            await hubNotificationSender.SendToGroupAsync(
                TopicAudience.Account(asker.UserId), "OnApprovalResolved",
                new ApprovalResolvedNotification("", approvalId, AnsweredElsewhere: answeredElsewhere));
            return;
        }

        if (context.TopicId is not { } topicId)
        {
            return;
        }

        sessionService.TryGetSession(topicId, out var session);

        try
        {
            var notification = new ApprovalResolvedNotification(
                topicId, approvalId, SpaceSlug: session?.SpaceSlug, AnsweredElsewhere: answeredElsewhere);
            if (session?.SpaceSlug is not { } spaceSlug)
            {
                await hubNotificationSender.SendAsync("OnApprovalResolved", notification);
                return;
            }

            var topic = await threadStore.GetTopicByChatIdAndThreadIdAsync(
                session.AgentId, session.ChatId, session.ThreadId);
            await hubNotificationSender.SendToTopicAsync(topic, spaceSlug, "OnApprovalResolved", notification);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to notify approval resolved for topic {TopicId}", topicId);
        }
    }

    // Which topic a pending approval was asked in, so whoever answers it can be held to that
    // topic's visibility. Null once it is answered, when it never was asked, and for a question
    // offered to a person rather than asked in a topic.
    public string? TopicOfApproval(string approvalId) =>
        _pendingApprovals.TryGetValue(approvalId, out var context) && !context.Offered ? context.TopicId : null;

    // Whose question a pending approval is: the one account that may answer it. Null for a question
    // acting for nobody, which its topic's own rule answers.
    public Account? AskerOf(string approvalId) =>
        _pendingApprovals.TryGetValue(approvalId, out var context) ? context.Asker : null;

    // The questions offered to one person, for a chat client catching up on what it missed while it
    // was not open.
    public IReadOnlyList<ToolApprovalRequestMessage> OfferedTo(string userId) =>
        [.. _pendingApprovals
            .Where(kv => kv.Value is { Offered: true, Asker: { } asker } && asker.UserId == userId)
            .Select(kv => new ToolApprovalRequestMessage(kv.Key, kv.Value.Requests, userId))];

    public bool IsApprovalPending(string approvalId)
    {
        return _pendingApprovals.ContainsKey(approvalId);
    }

    public ToolApprovalRequestMessage? GetPendingApprovalForTopic(string topicId)
    {
        var pending = _pendingApprovals
            .FirstOrDefault(kv => !kv.Value.Offered && kv.Value.TopicId == topicId);

        return pending.Key is null
            ? null
            : new ToolApprovalRequestMessage(pending.Key, pending.Value.Requests, pending.Value.Asker?.UserId);
    }

    // The topic is over — cancelled or deleted — so every prompt it raised is over with it. The
    // waiting tool call is told no, and the browsers showing the prompt are told the same way an
    // answer tells them: left up, the modal asks about a turn that no longer exists.
    public async Task CancelPendingApprovalsForTopicAsync(string topicId)
    {
        var expiredApprovals = _pendingApprovals
            .Where(kv => !kv.Value.Offered && kv.Value.TopicId == topicId)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var approvalId in expiredApprovals)
        {
            if (!_pendingApprovals.TryRemove(approvalId, out var context))
            {
                continue;
            }

            await NotifyResolvedAsync(context, approvalId, answeredElsewhere: false);

            context.TrySetResult(ToolApprovalResult.Rejected);
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