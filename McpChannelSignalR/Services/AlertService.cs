using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;

namespace McpChannelSignalR.Services;

// A timer ringing for one person, shown on every chat client they have open wherever they are —
// the account:{userId} group each connection joins as it arrives, whatever space — and pushed to
// their devices, since an alarm has to reach a phone in a pocket and a client in the background
// sounds nothing. Theirs alone to dismiss, from any of their clients; the dismissal is the offer's
// answer, and every client showing it is told it ended. An alert that ended elsewhere (dismissed on
// Telegram, ./dismiss, rung out) is taken down the same way.
public sealed class AlertService(
    IHubNotificationSender hub,
    IPushNotificationService push,
    ILogger<AlertService> logger)
{
    private readonly ConcurrentDictionary<string, Ringing> _ringing = new();

    public async Task<string> OfferAlertAsync(OfferAlertParams p, CancellationToken ct)
    {
        var ringing = new Ringing(p.Owner, p.Text, p.Kind);
        if (!_ringing.TryAdd(p.AlertId, ringing))
        {
            return ChannelProtocol.NoAnswer;
        }

        await hub.SendToGroupAsync(
            TopicAudience.Account(p.Owner.UserId), "OnAlertOffered",
            new AlertOfferedNotification(p.AlertId, p.Text, p.Kind), CancellationToken.None);
        await PushAsync(p);

        try
        {
            return await ringing.Answer.Task.WaitAsync(ct)
                ? ChannelProtocol.AlertDismissed
                : ChannelProtocol.NoAnswer;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await EndAsync(p.AlertId, dismissed: false);
            throw;
        }
    }

    // True when it was this person's to dismiss and still ringing; anyone else answers nothing, and
    // it keeps ringing for its owner.
    public async Task<bool> DismissAsync(string alertId, Account dismisser)
    {
        if (!_ringing.TryGetValue(alertId, out var ringing) || ringing.Owner != dismisser)
        {
            return false;
        }

        return await EndAsync(alertId, dismissed: true, answer: true);
    }

    public Task SettleAlertAsync(string alertId, bool dismissed) => EndAsync(alertId, dismissed);

    // What still rings for one person, for a chat client catching up on what it missed while it was
    // not open — the client the push brought up, above all.
    public IReadOnlyList<AlertOfferedNotification> OfferedTo(Account person) =>
        [.. _ringing
            .Where(entry => entry.Value.Owner == person)
            .Select(entry => new AlertOfferedNotification(entry.Key, entry.Value.Text, entry.Value.Kind))];

    private async Task<bool> EndAsync(string alertId, bool dismissed, bool answer = false)
    {
        if (!_ringing.TryRemove(alertId, out var ringing))
        {
            return false;
        }

        ringing.Answer.TrySetResult(answer);
        try
        {
            await hub.SendToGroupAsync(
                TopicAudience.Account(ringing.Owner.UserId), "OnAlertSettled",
                new AlertSettledNotification(alertId, dismissed), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Taking alert {AlertId} off its owner's chat clients failed", alertId);
        }

        return true;
    }

    private async Task PushAsync(OfferAlertParams p)
    {
        try
        {
            await push.SendToPersonAsync(
                p.Owner.UserId, SpaceConfig.DefaultSlug, p.Kind == AnnounceKind.Alarm ? "Alarm" : "Timer", p.Text, "/");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to push alert {AlertId} to its owner's devices", p.AlertId);
        }
    }

    private sealed record Ringing(Account Owner, string Text, AnnounceKind Kind)
    {
        public TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}