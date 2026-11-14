using System.Runtime.ExceptionServices;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

// Nobody approves for anyone else, and a person answers wherever they are. The question is put on
// the channel the turn's delivery anchors (where it always was), and — when the turn acts for an
// account — offered to that account on every other channel that can reach a person wherever they
// are (the chat client's notification, their Telegram private chat). The first answer wins; every
// other channel is told it was answered elsewhere and its call is called off.
//
// The anchor is authoritative about ending the question: its answer, its refusal and its time-out
// all end it, as they did before anything was offered. Only an anchor that could not show the
// question to anyone defers to the offers, for a window, so a voice turn whose satellite went away
// is not refused while its person holds Telegram — and is never held open for good either. An
// offer that is not answered never ends anything.
//
// A turn acting for nobody (the voice guest, a fire nobody stamped) has nobody to find elsewhere,
// so it is asked on the anchor alone, under that channel's own rule.
public sealed class ApprovalRelay(
    IChannelConnection anchor,
    // The channels that may be offered the question: the deployment's and the turn's household's own,
    // read as each question is asked, so a connection that appeared since is offered it too.
    IEnumerable<IChannelConnection> channels,
    ILogger logger,
    TimeProvider time,
    IAccountStanding? standing = null) : IToolApprovalHandler
{
    // How often a waiting question asks whether its asker is still somebody the agent acts for. The
    // standing is the household view's, kept a minute and dropped on the announcement, so this is the
    // grain a disabling reaches a question at, not a cost.
    public static readonly TimeSpan StandingCheck = TimeSpan.FromSeconds(15);

    // As long as Telegram has always waited for a tap: long enough to reach a phone, short enough
    // that a turn nobody could be asked in does not hold its conversation.
    public static readonly TimeSpan UnshownWindow = TimeSpan.FromMinutes(2);

    public async Task<ToolApprovalResult> RequestApprovalAsync(ApprovalAsk ask, CancellationToken ct)
    {
        if (ask.Asker is not { } asker)
        {
            return await anchor.AskApprovalAsync(ask, ct) ?? ToolApprovalResult.Rejected;
        }

        var asking = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var offered = channels.Where(c => c != anchor).ToList();
        var anchorTask = anchor.AskApprovalAsync(ask, asking.Token);
        var waiting = offered
            .Select(channel => (Channel: channel, Task: channel.OfferApprovalAsync(ask, asking.Token)))
            .Prepend((Channel: anchor, Task: anchorTask))
            .ToList();
        var window = Task.Delay(Timeout.InfiniteTimeSpan, asking.Token);
        // A member disabled while their question waits can answer it nowhere any more, and the agent
        // acts for them no longer: it is refused everywhere it shows instead of holding the turn
        // until they are re-enabled.
        var disabled = standing is null
            ? Task.Delay(Timeout.InfiniteTimeSpan, asking.Token)
            : DisabledAsync(asker, asking.Token);
        Exception? anchorFailure = null;

        try
        {
            while (waiting.Count > 0)
            {
                var done = await Task.WhenAny(waiting.Select(w => w.Task).Append(window).Append(disabled));
                ct.ThrowIfCancellationRequested();
                if (done == window)
                {
                    break;
                }

                if (done == disabled)
                {
                    logger.LogInformation(
                        "Approval {ApprovalId} is refused: its asker {UserId} was disabled while it waited",
                        ask.ApprovalId, asker.UserId);
                    _ = SettleAsync(ask, winner: null, ToolApprovalResult.Rejected, asking);
                    return ToolApprovalResult.Rejected;
                }

                // By position, never by task alone: an offer answered before it awaited anything is
                // the runtime's one cached task for its answer, so two channels that could not show
                // the question hand back the same task.
                var finished = waiting.FindIndex(w => w.Task == done);
                var (channel, task) = waiting[finished];
                waiting.RemoveAt(finished);
                var answer = AnswerOf(channel, task);
                if (answer is { } given)
                {
                    var result = await StillTheAskersAsync(ask, given, ct);
                    _ = SettleAsync(ask, channel, result, asking);
                    return result;
                }

                if (channel == anchor)
                {
                    window = Task.Delay(UnshownWindow, time, asking.Token);
                    anchorFailure = task.Exception?.InnerException;
                }
            }

            // Nobody answered anywhere. An anchor that failed fails the call as it always did, so a
            // channel that is not connected still reads as one; one that only could not show the
            // question is a refusal, as a question nobody can see always was (ADR-0035).
            await asking.CancelAsync();
            asking.Dispose();
            if (anchorFailure is not null)
            {
                ExceptionDispatchInfo.Throw(anchorFailure);
            }

            return ToolApprovalResult.Rejected;
        }
        catch
        {
            await asking.CancelAsync();
            asking.Dispose();
            throw;
        }
    }

    // Told where the turn asked alone: an auto-approval is what the turn is doing, shown with it.
    public Task NotifyAutoApprovedAsync(
        string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken cancellationToken) =>
        anchor.NotifyAutoApprovedAsync(conversationId, requests, cancellationToken);

    // A channel that failed, or was called off, has not answered; what it failed with is logged and
    // nothing more, since the question may still be answered somewhere else.
    private ToolApprovalResult? AnswerOf(IChannelConnection channel, Task<ToolApprovalResult?> task)
    {
        if (task.IsCompletedSuccessfully)
        {
            return task.Result;
        }

        if (task.Exception is { } failure)
        {
            logger.LogWarning(failure.InnerException ?? failure,
                "Approval on {ChannelId} failed; the question is left to the other channels", channel.ChannelId);
        }

        return null;
    }

    // Asked again each StandingCheck; a standing that cannot be read refuses nothing here — an outage
    // is not a disabling — and the answer, when it comes, is checked once more.
    private async Task DisabledAsync(Account asker, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(StandingCheck, time, ct);
            try
            {
                if (await standing!.StandingAsync(asker, ct) == AccountStanding.Disabled)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "The standing of {UserId} could not be read while their approval waits", asker.UserId);
            }
        }
    }

    // An answer that lets something through is the asker's word only while the agent still acts for
    // them: a channel may hold who a sender is for minutes (Telegram's link cache), so a member
    // disabled since could still approve there. One whose asker cannot be checked is refused, as a
    // turn whose person cannot be checked is. A refusal needs nobody's standing.
    private async Task<ToolApprovalResult> StillTheAskersAsync(ApprovalAsk ask, ToolApprovalResult given, CancellationToken ct)
    {
        if (standing is null || ask.Asker is not { } asker
            || given is not (ToolApprovalResult.Approved or ToolApprovalResult.ApprovedAndRemember))
        {
            return given;
        }

        try
        {
            if (await standing.StandingAsync(asker, ct) == AccountStanding.Active)
            {
                return given;
            }

            logger.LogInformation("Approval {ApprovalId} is refused: its asker {UserId} is disabled", ask.ApprovalId, asker.UserId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Approval {ApprovalId} is refused: the standing of {UserId} could not be read",
                ask.ApprovalId, asker.UserId);
        }

        return ToolApprovalResult.Rejected;
    }

    // Off the turn: the answer is in, and telling the others is no reason to make the tool wait.
    // Settled before being called off, so a channel still showing the question says who answered it
    // rather than only taking it down.
    // With no winner — refused for a disabling — every channel is told, the one the turn asked in too.
    private async Task SettleAsync(
        ApprovalAsk ask, IChannelConnection? winner, ToolApprovalResult result, CancellationTokenSource asking)
    {
        try
        {
            await Task.WhenAll(channels.Where(c => c != winner).Select(async channel =>
            {
                try
                {
                    await channel.SettleApprovalAsync(ask.ApprovalId, result, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Telling {ChannelId} that approval {ApprovalId} was answered elsewhere failed",
                        channel.ChannelId, ask.ApprovalId);
                }
            }));
        }
        finally
        {
            await asking.CancelAsync();
            asking.Dispose();
        }
    }
}