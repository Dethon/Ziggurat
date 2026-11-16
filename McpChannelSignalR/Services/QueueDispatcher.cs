using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;

namespace McpChannelSignalR.Services;

// Sends each queued message as the turn before it ends. A turn ends when the agent's send_reply
// finishes it, where there is no hub call and no connection in hand, so this is a collaborator of the
// stream service rather than a hub method: the stream service hands it each head it takes, and it
// sends the head from the snapshot taken as the message was queued. It depends on the stream service
// and never the other way round, so the two are not a cycle.
public sealed class QueueDispatcher(
    StreamService streamService,
    PersonTurns turns,
    QueueNews news,
    ILogger<QueueDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var turn in streamService.Dispatches.ReadAllAsync(stoppingToken))
            {
                // Each on its own: one topic's sender being vouched for holds up no other topic's.
                _ = DispatchAsync(turn);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // Vouched for afresh, then written, then sent: the person saw the message waiting, so it takes its
    // place in the conversation as its turn starts even when the turn is refused under it. Vouched for
    // before its turn starts, so a stop while the accounts service answers returns it to its sender
    // rather than leave it drawn on the stream and never sent. A queued message runs whether or not its
    // sender still has a page open, so nothing here is bound to one.
    private async Task DispatchAsync(QueuedTurn turn)
    {
        var (queued, stream) = turn;
        var sender = queued.Sender;
        await news.TellOrLogAsync(sender);
        try
        {
            var vouching = await turns.VouchingAsync(sender.UserId, sender.SessionId, CancellationToken.None);
            var started = await streamService.StartAsync(turn, async () =>
            {
                await turns.WriteMessageAsync(sender, queued.Message);
                switch (vouching)
                {
                    case NotVouched refused:
                        await turns.RefuseAsync(sender, refused.Refusal);
                        break;
                    case Vouched vouched:
                        await turns.EmitAsync(new VouchedSender(sender, vouched.Account), queued.Message, CancellationToken.None);
                        break;
                }
            });

            // Its sender's turn is running now, which the pages are told so its sender may steer it.
            if (started)
            {
                await news.TellOrLogAsync(sender);
            }
        }
        catch (Exception ex) when (streamService.IsRunning(stream))
        {
            // The turn it was given would otherwise hold the stream open for good.
            logger.LogError(ex, "Sending queued message {MessageId} in topic {TopicId} failed", queued.Id, sender.TopicId);
            await streamService.WriteReplyAsync(new SendReplyParams
            {
                ConversationId = sender.Conversation.ConversationId,
                Content = TopicRefusals.NotSent,
                ContentType = ReplyContentType.Error,
                IsComplete = true
            });
        }
    }
}